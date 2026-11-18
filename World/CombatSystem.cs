using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Per-frame intersection pass. Walks every published hitbox against every published
// hurtbox; on overlap with mismatched factions, dispatches OnHit to the hurtbox
// owner. Also applies the same hitboxes to tiles (which aren't IHittables — they're
// addressed by cell coords).
//
// Dedupe semantics:
//   Entities: each (HitId, Target) pair fires OnHit exactly once. A slash whose
//             hitbox broadcasts for 4 frames lands on a balloon once. The dedupe
//             table persists across frames while the HitId keeps broadcasting; it
//             prunes when the HitId disappears (attack ended).
//   Tiles:   No dedupe — multi-frame hitboxes accumulate damage on a tile every
//            frame they overlap. Preserves the progressive darkening visual.
// Occlusion: a hitbox published with an Origin only lands on cells / hurtboxes
//            it has line of sight to through the terrain (TileReach), and its
//            tile damage propagates nearest-first from that origin.
// Clank:     before any of that, opposing melee hitboxes (Hitbox.Clank) that touch
//            each other resolve first — see ResolveClanks.
// Instance-owned (not static): the (HitId → already-hit targets) dedupe table
// persists across frames, so it's simulation state that must be snapshot/restored
// for rollback. One CombatSystem per Simulation. The geometry helper below stays
// static — it's a pure function.
public sealed class CombatSystem
{
    // Set by Simulation from Stage.Coop: player factions don't hit each other. Fixed for
    // the match, so it is not snapshotted.
    public bool Coop { get; set; }

    private readonly Dictionary<int, HashSet<EntityId>> _hitDedupe = new();
    private readonly HashSet<int> _liveHitIds = new();
    // HitIds whose attack is paused, not over (attacker hitstop freezes the action, so
    // its hitbox doesn't publish). Exempt from the prune below so the dedupe set — and
    // the tile-pogo latch riding on it — survives the freeze; without this, the attack
    // re-hit the same target (and re-pogoed) the frame hitstop ended. Filled during the
    // player updates, consumed by the Apply in the same Step, so it is always empty at
    // a frame boundary and needs no snapshot.
    private readonly HashSet<int> _heldHitIds = new();
    private readonly List<int> _scratchPrune = new();
    // Per-frame recoil tally — accumulated Δv to apply to each hitbox's
    // attacker, keyed by HitId. Cleared at the start of every Apply. Attackers
    // read via PeekRecoil in their ApplyActionForces hook — that runs in frame
    // N+1 before frame N+1's Apply clears the dict, so the tally is effectively
    // a 1-frame inbox from CombatSystem to the action layer. Snapshotted
    // (CaptureRecoil) like the hit-confirm inbox — it's a pending N→N+1 message.
    private readonly Dictionary<int, Vector2> _recoilByHitId = new();
    // Per-frame hit-confirm tally — count of ENTITIES a hitbox connected with this
    // frame, keyed by HitId. Same 1-frame-inbox lifecycle as _recoilByHitId: cleared
    // at the start of every Apply, populated as entity hits land, read the next frame
    // by the attacker's action. Because entity hits are deduped per (HitId, Target),
    // a given target only counts on the frame it first connects — so an attack that
    // polls PeekHits each of its active/recovery frames sees the connection exactly
    // once and can latch a "did I hit?" flag (COMBAT_FEEL_PLAN Phase 3 hit-confirm).
    private readonly Dictionary<int, int> _entityHitsByHitId = new();
    // Attacker-hitstop inbox (symmetric hitlag, 2026-09-01) — the freeze (seconds)
    // each HitId's connects earned this frame, max-merged across victims from the same
    // curve the victim gets (CombatState.HitstopSecondsFor). Same 1-frame N→N+1
    // lifecycle as the recoil inbox: PlayerCharacter reads PeekHitstop at the END of
    // its next Update (after recoil is applied, so the freeze can't eat the pogo) and
    // stamps its own CombatState. Struggle hits (GrabStrengthDamage > 0) contribute
    // nothing — a struggle must have zero effect on either side. Snapshotted below.
    private readonly Dictionary<int, float> _hitstopByHitId = new();
    // Per-hitbox scratch: the Solid cells it overlaps, keyed for nearest-first
    // ordering when the hitbox is occluded (see TileReach).
    private readonly List<(float key, int gtx, int gty)> _scratchCells = new();

    // Cosmetic: a player-owned hitbox with an origin touched terrain this frame —
    // (HitId, contact point on the nearest reachable cell, unit direction into the
    // surface). Raised every frame of overlap and before damage, so it also fires
    // for a cell the hit breaks. Subscribers dedupe by HitId; nothing sim-side reads it.
    public event Action<int, Vector2, Vector2> OnTerrainStrike;

    // Cosmetic: two attacks clanked this frame — (HitId a, HitId b, contact point,
    // metallic = both were blades). Raised once per clank (a cancelled attack never
    // clanks again), so it needs no dedupe beyond the presentation log's (frame, id).
    public event Action<int, int, Vector2, bool> OnClank;

    // Two clanking attacks within this fraction of each other's BodyDamage cancel
    // each other; past it the stronger one goes through and only the weaker stops.
    public const float ClankDamageBand = 0.2f;
    // Pseudo-target written into a cancelled attack's dedupe set: "this HitId lost a
    // clank, it lands on no entity for the rest of its broadcast". Riding the dedupe
    // table means it snapshots and prunes with it. Index -1 is never minted (ids start
    // at 1, and None — the tile-pogo latch — is 0).
    private static readonly EntityId ClankedMark = new(-1);
    // Dedupe-set marker: this attack has already spent its Hitbox.TileImpact crater.
    private static readonly EntityId TileImpactMark = new(-2);
    private readonly List<(int gtx, int gty)> _impactSeedScratch = new(1);
    private readonly List<int> _scratchClank = new();

    // Keep this HitId's dedupe entry alive through a frame it doesn't broadcast.
    public void HoldHitId(int hitId) => _heldHitIds.Add(hitId);

    public Vector2 PeekRecoil(int hitId)
        => _recoilByHitId.TryGetValue(hitId, out var v) ? v : Vector2.Zero;

    // Entities this HitId connected with on the LAST resolved frame. Read by an
    // attacker's action (one frame after the hit) to confirm a strike landed —
    // e.g. to gate a combo follow-up on a hit (whiff-punish), per Phase 3.
    public int PeekHits(int hitId)
        => _entityHitsByHitId.TryGetValue(hitId, out var n) ? n : 0;

    // Attacker hitstop earned by this HitId's connects on the LAST resolved frame,
    // in seconds (0 = none). See _hitstopByHitId.
    public float PeekHitstop(int hitId)
        => _hitstopByHitId.TryGetValue(hitId, out var s) ? s : 0f;

    // `resolve` maps a hurtbox's owning EntityId back to the live IHittable so OnHit
    // can be dispatched. The dedupe table and snapshots key on EntityId (value
    // identity), but the actual damage callback still needs the object. Callers own
    // the entity set, so they supply the lookup.
    public void Apply(ChunkMap chunks, HitboxWorld hitboxes, HurtboxWorld hurtboxes,
                      Func<EntityId, IHittable> resolve)
    {
        _liveHitIds.Clear();
        _recoilByHitId.Clear();
        _entityHitsByHitId.Clear();
        _hitstopByHitId.Clear();
        _resolveDuringApply = resolve;

        ResolveClanks(chunks, hitboxes, resolve);

        foreach (var hit in hitboxes.All)
        {
            _liveHitIds.Add(hit.HitId);

            // Sensors report and do nothing else — no dedupe entry, no effects.
            if (hit.Sensor) { Sense(in hit, chunks, hurtboxes, resolve); continue; }

            // Narrow-phase polygon shape — compute world-space vertices + edge axes
            // once per hitbox, reused across all tile / entity overlap checks.
            Vector2[] hitVerts = null;
            Vector2[] hitAxes  = null;
            if (hit.Shape != null)
            {
                hitVerts = hit.Shape.GetVertices(hit.ShapePos, hit.ShapeRotation);
                hitAxes  = hit.Shape.GetAxes(hitVerts);
            }

            // Per-attack dedupe set, shared by the entity path AND the tile-recoil
            // latch below (fetched up here so both can see it). Persists across the
            // attack's broadcast window; pruned when the HitId stops broadcasting.
            if (!_hitDedupe.TryGetValue(hit.HitId, out var alreadyHit))
            {
                alreadyHit = new HashSet<EntityId>();
                _hitDedupe[hit.HitId] = alreadyHit;
            }

            // --- Tile path (cumulative; same as the old DamageSystem) -----------
            if (hit.Targets != HitTargets.EntitiesOnly)
            {
                // Collision-mode tile recoil resolves ONCE per hitbox per frame —
                // a wall face of N cells is one surface, not N collisions. The
                // cell loop only records the bounciest eligible material here;
                // TileRecoil below turns it into a single reflected bounce.
                // (< 0 ⇒ no eligible surface contacted this frame.)
                float surfaceRestitution = -1f;

                // Gather the Solid cells the hitbox overlaps (DamageCell is a no-op
                // on anything else). An occluded hitbox then visits them nearest
                // the origin first, so a cell that breaks frees the sightline to
                // the one behind it within this same pass — a hit strong enough
                // to break the front layer punches on through.
                _scratchCells.Clear();
                int gtx0 = (int)MathF.Floor(hit.Region.Left   / Chunk.TileSize);
                int gtx1 = (int)MathF.Floor(hit.Region.Right  / Chunk.TileSize);
                int gty0 = (int)MathF.Floor(hit.Region.Top    / Chunk.TileSize);
                int gty1 = (int)MathF.Floor(hit.Region.Bottom / Chunk.TileSize);
                for (int gtx = gtx0; gtx <= gtx1; gtx++)
                for (int gty = gty0; gty <= gty1; gty++)
                {
                    if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
                    if (hit.TileMaxMaterialHP > 0f
                        && TileDamage.MaxHPFor(chunks.GetCellType(gtx, gty)) > hit.TileMaxMaterialHP) continue;
                    if (hit.Shape != null)
                    {
                        var tileAABB = new BoundingBox(
                            gtx * Chunk.TileSize,       gty * Chunk.TileSize,
                            (gtx + 1) * Chunk.TileSize, (gty + 1) * Chunk.TileSize);
                        if (!OverlapsPolyAABB(hitVerts, hitAxes, tileAABB)) continue;
                    }
                    float key = hit.Occluded ? TileReach.DistanceKey(hit.Origin, gtx, gty) : 0f;
                    _scratchCells.Add((key, gtx, gty));
                }
                if (hit.Occluded) _scratchCells.Sort();

                // Tile impact (Hitbox.TileImpact): one crater per attack, on first contact,
                // BEFORE the direct pass so the field reads the rock as the strike found it.
                if (hit.TileImpact > 0f && _scratchCells.Count > 0 && !alreadyHit.Contains(TileImpactMark)
                    && ApplyTileImpact(in hit, chunks))
                    alreadyHit.Add(TileImpactMark);

                // Terrain-strike cue: of the reachable cells, the one nearest the
                // hitbox's centre (where the blade actually is, not the attacker's feet).
                bool wantStrike = OnTerrainStrike != null && hit.Occluded && Factions.IsPlayer(hit.Owner);
                var aim = new Vector2((hit.Region.Left + hit.Region.Right) * .5f,
                                      (hit.Region.Top + hit.Region.Bottom) * .5f);
                float bestStrike = float.MaxValue;
                Vector2 strikeAt = default;
                foreach (var (_, gtx, gty) in _scratchCells)
                {
                    // A tile impact above may already have cleared this cell.
                    if (hit.TileImpact > 0f && chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
                    if (hit.Occluded && !TileReach.IsCellReachable(chunks, hit.Origin, gtx, gty))
                        continue;
                    if (wantStrike)
                    {
                        float ts = Chunk.TileSize;
                        var p = new Vector2(Math.Clamp(aim.X, gtx * ts, (gtx + 1) * ts),
                                            Math.Clamp(aim.Y, gty * ts, (gty + 1) * ts));
                        float d = Vector2.DistanceSquared(p, aim);
                        if (d < bestStrike) { bestStrike = d; strikeAt = p; }
                    }
                    // Snapshot the material BEFORE DamageCell, since a break clears
                    // the cell and we'd lose the type info for the hardness gate.
                    var typeBefore = chunks.GetCellType(gtx, gty);
                    // DamageCell returns true iff the tile broke this call.
                    bool broke = chunks.DamageCell(gtx, gty, hit.Damage);
                    if (hit.RecoilScale > 0f)
                    {
                        // Two gates, both must allow before crediting recoil:
                        //   BreakProtected ⇒ skip cells that this hit destroyed.
                        //   MinMaterialHP  ⇒ skip cells whose material is below the
                        //                    hardness floor (e.g. sand under a stab).
                        bool breakAllows = !hit.RecoilBreakProtected || !broke;
                        bool hardEnough  = TileDamage.MaxHPFor(typeBefore) > hit.RecoilMinMaterialHP;
                        if (!breakAllows || !hardEnough) continue;
                        if (hit.Mode == KnockbackMode.Collision)
                        {
                            float e = MaterialStrengths.RestitutionFor(typeBefore);
                            if (e > surfaceRestitution) surfaceRestitution = e;
                        }
                        else
                            // Legacy impulse recoil: authored vector, per cell, per frame.
                            AccumulateRecoil(in hit, -hit.KnockbackImpulse * hit.RecoilScale);
                    }
                }

                if (bestStrike < float.MaxValue)
                {
                    var into = strikeAt - hit.Origin;
                    into = into.LengthSquared() > 1e-4f ? Vector2.Normalize(into) : hit.StrikeDir;
                    OnTerrainStrike(hit.HitId, strikeAt, into);
                }

                // One pogo per ATTACK, not per frame: the authored swing speed in
                // StrikeVelocity re-adds every frame the hitbox re-publishes, so
                // closing speed alone can't self-limit a multi-frame overlap (the
                // bounce only subtracts the attacker's body velocity). Latch on the
                // attack's dedupe set using EntityId.None as the "tile surface"
                // pseudo-target — None never collides with a live entity, and the
                // latch snapshot/restores for free with the Dedupe table.
                if (surfaceRestitution >= 0f && !alreadyHit.Contains(EntityId.None))
                {
                    AccumulateRecoil(in hit, HitResolver.TileRecoil(in hit, surfaceRestitution));
                    alreadyHit.Add(EntityId.None);
                }
            }

            // --- Entity path (deduped per (HitId, Target) via `alreadyHit`) ------
            // An attack that lost a clank keeps working on tiles but no longer on bodies.
            if (hit.Targets != HitTargets.TilesOnly && !alreadyHit.Contains(ClankedMark))
            {
                foreach (var hb in hurtboxes.All)
                {
                    if (hit.Owner == hb.Owner && !AcrossTeams(in hit, in hb, resolve)) continue;
                    // Co-op: the two players are allies — no player-faction hit lands on
                    // the other player (slashes, projectiles and thrown blocks all carry
                    // their thrower's player faction as Owner).
                    if (Coop && Factions.IsPlayer(hit.Owner) && Factions.IsPlayer(hb.Owner)) continue;
                    if (alreadyHit.Contains(hb.Target)) continue;
                    if (!HitboxWorld.Overlaps(hit.Region, hb.Region)) continue;
                    if (hit.Shape != null && !OverlapsPolyAABB(hitVerts, hitAxes, hb.Region)) continue;
                    // Terrain between the origin and the target blocks the hit. Runs
                    // after the tile pass above, so a swing that just broke through a
                    // wall reaches whoever was behind it this same frame.
                    if (hit.Occluded && !TileReach.IsRegionReachable(chunks, hit.Origin, hb.Region)) continue;
                    var target = resolve(hb.Target);
                    if (target == null) continue;   // owner despawned this frame — skip
                    // OnHit returns the impulse actually delivered (HitResolver) —
                    // Impulse-mode targets echo back the authored KnockbackImpulse,
                    // so recoil there is unchanged; Collision-mode targets return the
                    // resolved J·n, so a heavy target that barely moved kicks the
                    // attacker back hard (Newton's third law for real this time).
                    var delivered = target.OnHit(hit, hb);
                    alreadyHit.Add(hb.Target);
                    _entityHitsByHitId.TryGetValue(hit.HitId, out var prevHits);
                    _entityHitsByHitId[hit.HitId] = prevHits + 1;
                    if (hit.GrabStrengthDamage <= 0f)
                    {
                        float stop = CombatState.HitstopSecondsFor(HitResolver.ImpactStrength(delivered));
                        _hitstopByHitId.TryGetValue(hit.HitId, out var curStop);
                        if (stop > curStop) _hitstopByHitId[hit.HitId] = stop;
                    }
                    if (hit.RecoilScale > 0f)
                        AccumulateRecoil(in hit, -delivered * hit.RecoilScale);
                }
            }
        }
        
        // Prune dedupe entries for HitIds that didn't broadcast this frame —
        // their attack is over, future hitboxes will use fresh HitIds anyway.
        // Held HitIds (attacker in hitstop) are paused, not over.
        _scratchPrune.Clear();
        foreach (var k in _hitDedupe.Keys)
            if (!_liveHitIds.Contains(k) && !_heldHitIds.Contains(k)) _scratchPrune.Add(k);
        foreach (var k in _scratchPrune) _hitDedupe.Remove(k);
        _heldHitIds.Clear();
    }

    // ── Sensors (Hitbox.Sensor) ─────────────────────────────────────────────────
    // Earliest touch of the hitbox swept along Sweep, against Solid cells and against the
    // hurtboxes the hitbox could hit, delivered to the Source's IContactSensor. The swept
    // shape is the hitbox's polygon Shape when it has one (a rounded sensor is a regular
    // polygon, as the slashes' arcs are), else its Region box. Two rules keep a sensor
    // from firing on terrain it is leaving or tunnelling out of:
    //   • behind the motion — a cell the shape ALREADY overlaps at t = 0 counts only if
    //     its nearest point lies ahead of the sweep (a clod lifted off a floor and thrown
    //     away from it doesn't touch that floor); with no sweep, any overlap counts.
    //   • buried — a touch at a moment the shape's CENTER is inside a Solid cell doesn't
    //     count, so something released inside rock flies out instead of stopping in it;
    //     the rock it exits is then behind it.
    // Only Solid is terrain here, as in the tile path (sprouts and spikes aren't).
    // Touching (shared edge) is not overlap. Ties keep the first found: cells in
    // ascending gtx/gty order, then hurtboxes in publish order — deterministic.
    private readonly Vector2[] _sensorBox = new Vector2[4];
    private static readonly Vector2[] BoxAxes = { Vector2.UnitX, Vector2.UnitY };

    private void Sense(in Hitbox hit, ChunkMap chunks, HurtboxWorld hurtboxes,
                       Func<EntityId, IHittable> resolve)
    {
        if (resolve(hit.Source) is not IContactSensor sensor) return;

        var r = hit.Region;
        var d = hit.Sweep;
        bool moving = d.LengthSquared() > 1e-8f;

        Vector2[] verts, axes;
        Vector2 c0;
        if (hit.Shape != null)
        {
            verts = hit.Shape.GetVertices(hit.ShapePos, hit.ShapeRotation);
            axes  = hit.Shape.GetAxes(verts);
            c0    = hit.ShapePos;
        }
        else
        {
            _sensorBox[0] = new Vector2(r.Left,  r.Top);
            _sensorBox[1] = new Vector2(r.Right, r.Top);
            _sensorBox[2] = new Vector2(r.Right, r.Bottom);
            _sensorBox[3] = new Vector2(r.Left,  r.Bottom);
            verts = _sensorBox;
            axes  = BoxAxes;
            c0    = new Vector2((r.Left + r.Right) * 0.5f, (r.Top + r.Bottom) * 0.5f);
        }

        bool     found  = false;
        float    bestT  = float.MaxValue;
        Vector2  bestN  = default;
        EntityId bestId = EntityId.None;

        if (hit.Targets != HitTargets.EntitiesOnly && chunks != null)
        {
            float ts = Chunk.TileSize;
            int gx0 = (int)MathF.Floor(MathF.Min(r.Left,   r.Left   + d.X) / ts);
            int gx1 = (int)MathF.Floor(MathF.Max(r.Right,  r.Right  + d.X) / ts);
            int gy0 = (int)MathF.Floor(MathF.Min(r.Top,    r.Top    + d.Y) / ts);
            int gy1 = (int)MathF.Floor(MathF.Max(r.Bottom, r.Bottom + d.Y) / ts);
            for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (chunks.GetCellState(gx, gy) != TileState.Solid) continue;
                var cell = new BoundingBox(gx * ts, gy * ts, (gx + 1) * ts, (gy + 1) * ts);
                if (!SweptEntry(verts, axes, d, cell, out float t, out var n)) continue;
                if (t >= bestT) continue;
                if (t <= 0f && moving)
                {
                    var near = new Vector2(Math.Clamp(c0.X, cell.Left, cell.Right),
                                           Math.Clamp(c0.Y, cell.Top,  cell.Bottom));
                    if (Vector2.Dot(near - c0, d) <= 0f) continue;            // behind
                }
                var at = c0 + d * t;
                if (chunks.GetCellState((int)MathF.Floor(at.X / ts),
                                        (int)MathF.Floor(at.Y / ts)) == TileState.Solid) continue;   // buried
                found = true; bestT = t; bestN = n; bestId = EntityId.None;
            }
        }

        if (hit.Targets != HitTargets.TilesOnly && hurtboxes != null)
        {
            foreach (var hb in hurtboxes.All)
            {
                if (hb.Target == hit.Source) continue;
                if (hit.Owner == hb.Owner && !AcrossTeams(in hit, in hb, resolve)) continue;
                if (Coop && Factions.IsPlayer(hit.Owner) && Factions.IsPlayer(hb.Owner)) continue;
                if (!SweptEntry(verts, axes, d, hb.Region, out float t, out var n)) continue;
                if (t >= bestT) continue;
                found = true; bestT = t; bestN = n; bestId = hb.Target;
            }
        }

        if (found) sensor.OnSensorContact(in hit, new SensorContact(bestT, c0 + d * bestT, bestN, bestId));
    }

    // When does a convex shape (world vertices `verts`, edge normals `axes`) moving by `d`
    // over t ∈ [0,1] first overlap the box `target`? Swept SAT: on every separating axis
    // candidate (the shape's edge normals plus the box's x/y) the projections overlap
    // over some t-interval; the shapes overlap where all of those intervals do. t is
    // clamped to 0 for a starting overlap. `n` points out of the target: the axis whose
    // interval opened last, or for a starting overlap the shallowest penetration.
    private static bool SweptEntry(Vector2[] verts, Vector2[] axes, Vector2 d, BoundingBox target,
                                   out float t, out Vector2 n)
    {
        t = 0f; n = default;
        float tIn = float.NegativeInfinity, tOut = float.PositiveInfinity;
        Vector2 inAxis = default;
        float   pen = float.MaxValue;
        Vector2 penN = default;
        for (int pass = 0; pass < 2; pass++)
        {
            var list = pass == 0 ? axes : BoxAxes;
            foreach (var ax in list)
            {
                var (aMin, aMax) = Polygon.Project(verts, ax);
                float c1 = target.Left  * ax.X + target.Top    * ax.Y;
                float c2 = target.Right * ax.X + target.Top    * ax.Y;
                float c3 = target.Right * ax.X + target.Bottom * ax.Y;
                float c4 = target.Left  * ax.X + target.Bottom * ax.Y;
                float bMin = MathF.Min(MathF.Min(c1, c2), MathF.Min(c3, c4));
                float bMax = MathF.Max(MathF.Max(c1, c2), MathF.Max(c3, c4));
                float s = Vector2.Dot(d, ax);

                float lo, hi;
                if (MathF.Abs(s) < 1e-6f)
                {
                    if (aMin >= bMax || aMax <= bMin) return false;   // never overlaps on this axis
                    lo = float.NegativeInfinity; hi = float.PositiveInfinity;
                }
                else
                {
                    float t1 = (bMin - aMax) / s, t2 = (bMax - aMin) / s;
                    lo = MathF.Min(t1, t2); hi = MathF.Max(t1, t2);
                }
                if (lo > tIn) { tIn = lo; inAxis = s > 0f ? -ax : ax; }
                if (hi < tOut) tOut = hi;
                if (tIn >= tOut) return false;

                // Starting-overlap penetration on this axis (only meaningful if t=0 overlaps).
                float pMin = aMax - bMin, pMax = bMax - aMin;
                if (pMin < pen) { pen = pMin; penN = -ax; }
                if (pMax < pen) { pen = pMax; penN =  ax; }
            }
        }
        if (tOut <= 0f || tIn > 1f) return false;
        if (tIn > 0f) { t = tIn; n = inAxis; }
        else          { t = 0f;  n = penN;   }
        return true;
    }

    // ── Clank (todo.txt D) ──────────────────────────────────────────────────────
    // Hitbox-vs-hitbox, run before the hurtbox pass so a clanked swing can't also land
    // this frame. Two melee hitboxes (Clank != None, can reach entities) from opposing
    // sides — the same faction/team/co-op gate as a hit — that overlap, with the
    // contact point in line of sight of each origin when occluded. By BodyDamage: within ClankDamageBand
    // both are cancelled, otherwise only the weaker. A cancelled attacker gets exactly
    // what a parried one gets (PlayerCharacter.OnHit's guard early-out echoes the
    // authored impulse): recoil off its KnockbackImpulse and the hitstop that impulse
    // earns. Nothing else about the attacker changes — its action plays on.
    private void ResolveClanks(ChunkMap chunks, HitboxWorld hitboxes, Func<EntityId, IHittable> resolve)
    {
        var all = hitboxes.All;
        _scratchClank.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            var h = all[i];
            if (h.Clank == ClankKind.None || h.Targets == HitTargets.TilesOnly || h.GrabStrengthDamage > 0f) continue;
            if (Cancelled(h.HitId)) continue;
            _scratchClank.Add(i);
        }
        if (_scratchClank.Count < 2) return;

        for (int x = 0; x < _scratchClank.Count; x++)
        for (int y = x + 1; y < _scratchClank.Count; y++)
        {
            var a = all[_scratchClank[x]];
            var b = all[_scratchClank[y]];
            if (a.HitId == b.HitId) continue;
            if (Cancelled(a.HitId) || Cancelled(b.HitId)) continue;   // settled earlier this pass
            if (!Opposed(in a, in b, resolve)) continue;
            if (!HitboxWorld.Overlaps(a.Region, b.Region)) continue;
            if (!ShapesOverlap(in a, in b)) continue;
            // Where the two meet: the centre of the AABB overlap. Each side must see it.
            var r0 = a.Region; var r1 = b.Region;
            var at = new Vector2((MathF.Max(r0.Left, r1.Left) + MathF.Min(r0.Right, r1.Right)) * 0.5f,
                                 (MathF.Max(r0.Top, r1.Top) + MathF.Min(r0.Bottom, r1.Bottom)) * 0.5f);
            // (IsPointReachable doesn't test the point's own cell; blades meeting
            // inside a wall cell between the fighters didn't meet.)
            if ((a.Occluded || b.Occluded)
                && chunks.GetCellState((int)MathF.Floor(at.X / Chunk.TileSize),
                                       (int)MathF.Floor(at.Y / Chunk.TileSize)) == TileState.Solid) continue;
            if (a.Occluded && !TileReach.IsPointReachable(chunks, a.Origin, at)) continue;
            if (b.Occluded && !TileReach.IsPointReachable(chunks, b.Origin, at)) continue;

            float da = a.BodyDamage, db = b.BodyDamage;
            bool even = MathF.Abs(da - db) <= ClankDamageBand * MathF.Max(da, db);
            if (even || da < db) CancelByClank(in a);
            if (even || db < da) CancelByClank(in b);

            OnClank?.Invoke(a.HitId, b.HitId, at, a.Clank == ClankKind.Blade && b.Clank == ClankKind.Blade);
        }
    }

    private bool Cancelled(int hitId)
        => _hitDedupe.TryGetValue(hitId, out var set) && set.Contains(ClankedMark);

    // Would a hit from one land on the other's owner? The hit pass's gate, by faction.
    private bool Opposed(in Hitbox a, in Hitbox b, Func<EntityId, IHittable> resolve)
    {
        if (Coop && Factions.IsPlayer(a.Owner) && Factions.IsPlayer(b.Owner)) return false;
        if (a.Owner != b.Owner) return true;
        return a.Owner == Faction.Enemy
            && resolve(a.Source) is Entity ea && resolve(b.Source) is Entity eb && ea.Team != eb.Team;
    }

    private void CancelByClank(in Hitbox hit)
    {
        if (!_hitDedupe.TryGetValue(hit.HitId, out var set))
        {
            set = new HashSet<EntityId>();
            _hitDedupe[hit.HitId] = set;
        }
        set.Add(ClankedMark);
        float stop = CombatState.HitstopSecondsFor(HitResolver.ImpactStrength(hit.KnockbackImpulse));
        _hitstopByHitId.TryGetValue(hit.HitId, out var curStop);
        if (stop > curStop) _hitstopByHitId[hit.HitId] = stop;
        if (hit.RecoilScale > 0f)
            AccumulateRecoil(in hit, -hit.KnockbackImpulse * hit.RecoilScale);
    }

    // Narrow phase for a hitbox pair: polygon SAT where either has a Shape, else the
    // AABBs (already known to overlap).
    private static bool ShapesOverlap(in Hitbox a, in Hitbox b)
    {
        if (a.Shape == null && b.Shape == null) return true;
        if (a.Shape == null || b.Shape == null)
        {
            var poly = a.Shape != null ? a : b;
            var box  = a.Shape != null ? b.Region : a.Region;
            var v = poly.Shape.GetVertices(poly.ShapePos, poly.ShapeRotation);
            return OverlapsPolyAABB(v, poly.Shape.GetAxes(v), box);
        }
        var va = a.Shape.GetVertices(a.ShapePos, a.ShapeRotation);
        var vb = b.Shape.GetVertices(b.ShapePos, b.ShapeRotation);
        return !SeparatedOn(a.Shape.GetAxes(va), va, vb) && !SeparatedOn(b.Shape.GetAxes(vb), va, vb);
    }

    private static bool SeparatedOn(Vector2[] axes, Vector2[] va, Vector2[] vb)
    {
        foreach (var axis in axes)
        {
            var (amin, amax) = Polygon.Project(va, axis);
            var (bmin, bmax) = Polygon.Project(vb, axis);
            if (amax < bmin || bmax < amin) return true;
        }
        return false;
    }

    // The faction gate above skips any pair whose Owner factions match — self-immunity,
    // and why the stock enemies (all Faction.Enemy) never hit each other. Fighters
    // (Plans/FIGHTER_DESIGN_PLAN.md §5.3) are Faction.Enemy too, so between two
    // Enemy-faction parties the TEAM decides instead: the pair resolves when the
    // attacker (hit.Source — the enemy, or the projectile carrying its spawner's team)
    // and the target are both live entities on different teams. Every stock enemy and
    // its projectiles share Teams.Enemies, so nothing that resolved before resolves
    // differently now. Scoped to Faction.Enemy so player/neutral pairs keep the plain
    // faction rule. Consequence: same-team fighters are immune to each other's hits.
    private static bool AcrossTeams(in Hitbox hit, in Hurtbox hb, Func<EntityId, IHittable> resolve)
    {
        if (hit.Owner != Faction.Enemy) return false;
        return resolve(hit.Source) is Entity attacker
            && resolve(hb.Target)  is Entity target
            && attacker.Team != target.Team;
    }

    // ── Snapshot/restore (roadmap goal 4 §H) ────────────────────────────────────
    // The cross-frame (HitId → already-hit targets) table is the only durable combat
    // state. It now keys on EntityId (value identity), so capture/restore is a plain
    // value copy of each set — no id-of / resolve callbacks, and no staleness across a
    // restore (a rehydrated entity carries the same EntityId). _liveHitIds /
    // _scratchPrune are per-Apply scratch and need no snapshot. Order within a set
    // doesn't matter — it's a membership test (alreadyHit.Contains).
    public Dictionary<int, EntityId[]> CaptureDedupe()
    {
        var outMap = new Dictionary<int, EntityId[]>(_hitDedupe.Count);
        foreach (var (hitId, set) in _hitDedupe)
        {
            var ids = new EntityId[set.Count];
            set.CopyTo(ids);
            outMap[hitId] = ids;
        }
        return outMap;
    }

    public void RestoreDedupe(Dictionary<int, EntityId[]> data)
    {
        _hitDedupe.Clear();
        if (data == null) return;
        foreach (var (hitId, ids) in data)
            _hitDedupe[hitId] = new HashSet<EntityId>(ids);
    }

    // The hit-confirm tally IS durable across a snapshot boundary even though it's
    // rebuilt every Apply: it's a frame-N→frame-N+1 message (the attacker reads it
    // the frame after the hit lands — see PeekHits / SlashLikeAction). A snapshot
    // taken between the hit's Apply and the attacker's next read must carry it, or the
    // replayed run would miss a connection and diverge (the combo gate latches off it).
    // _recoilByHitId is the same kind of inbox and carries real data now that the
    // stab's tile recoil is live — so it gets the same treatment below.
    public Dictionary<int, int> CaptureHitConfirm() => new(_entityHitsByHitId);

    public Dictionary<int, Vector2> CaptureRecoil() => new(_recoilByHitId);

    public Dictionary<int, float> CaptureHitstop() => new(_hitstopByHitId);

    public void RestoreHitstop(Dictionary<int, float> data)
    {
        _hitstopByHitId.Clear();
        if (data == null) return;
        foreach (var (hitId, s) in data) _hitstopByHitId[hitId] = s;
    }

    public void RestoreRecoil(Dictionary<int, Vector2> data)
    {
        _recoilByHitId.Clear();
        if (data == null) return;
        foreach (var (hitId, v) in data) _recoilByHitId[hitId] = v;
    }

    public void RestoreHitConfirm(Dictionary<int, int> data)
    {
        _entityHitsByHitId.Clear();
        if (data == null) return;
        foreach (var (hitId, n) in data) _entityHitsByHitId[hitId] = n;
    }

    // A victim STOPPED a hit with its guard (PlayerCharacter.OnHit, called from inside the
    // OnHit dispatch below): hand the attacker its share - pushback, the same freeze, the
    // slide - the same frame, through IHittable.OnGuardStopped. Resolved with the Apply
    // call's own resolver; outside Apply there is no attacker to tell.
    private Func<EntityId, IHittable> _resolveDuringApply;
    public void NotifyAttackerGuardStopped(in Hitbox hit, Vector2 push, float hitstopSeconds, float slideSeconds)
        => _resolveDuringApply?.Invoke(hit.Source)?.OnGuardStopped(push, hitstopSeconds, slideSeconds);

    // Spend hit.TileImpact as a crater. The seed is the gathered cell furthest UPSTREAM
    // along the strike (smallest projection on its direction) that the hit can reach —
    // the first rock the strike meets. False when no cell is reachable, so the attack
    // keeps its crater for a later frame.
    private bool ApplyTileImpact(in Hitbox hit, ChunkMap chunks)
    {
        var dir = hit.StrikeDir;
        if (dir.LengthSquared() < 1e-6f) dir = hit.KnockbackImpulse;
        if (dir.LengthSquared() < 1e-6f) return false;
        dir.Normalize();

        bool found = false;
        float best = float.MaxValue;
        int sx = 0, sy = 0;
        foreach (var (_, gtx, gty) in _scratchCells)
        {
            if (hit.Occluded && !TileReach.IsCellReachable(chunks, hit.Origin, gtx, gty)) continue;
            float along = (gtx + 0.5f) * dir.X + (gty + 0.5f) * dir.Y;
            if (along < best) { best = along; sx = gtx; sy = gty; found = true; }
        }
        if (!found) return false;

        _impactSeedScratch.Clear();
        _impactSeedScratch.Add((sx, sy));
        ImpactSpringField.Apply(chunks, sx, sy, dir, hit.TileImpact, _impactSeedScratch);
        return true;
    }

    // The cap (Hitbox.MaxRecoilSpeed) is applied to the running TALLY, not to each
    // contribution: the stab's tile bounce and its entity recoil can land on the same
    // frame, and the legacy path sums per cell, so clamping each piece alone would
    // still let the total overshoot. Direction is preserved — only the magnitude is
    // brought down to the ceiling.
    private void AccumulateRecoil(in Hitbox hit, Vector2 impulse)
    {
        int hitId = hit.HitId;
        _recoilByHitId.TryGetValue(hitId, out var cur);
        var total = cur + impulse;
        float cap = hit.MaxRecoilSpeed;
        if (cap > 0f)
        {
            float len = total.Length();
            if (len > cap) total *= cap / len;
        }
        _recoilByHitId[hitId] = total;
    }

    // SAT: arbitrary convex polygon (given by its pre-computed world vertices +
    // edge normals) vs world-axis AABB. Returns true if they overlap.
    // Axes tested: polygon's edge normals + world X + world Y. For each, both
    // shapes are projected to a [min, max] interval; any disjoint interval means
    // no overlap.
    private static bool OverlapsPolyAABB(Vector2[] polyVerts, Vector2[] polyAxes, BoundingBox aabb)
    {
        // Polygon-normal axes
        for (int a = 0; a < polyAxes.Length; a++)
        {
            var axis = polyAxes[a];
            var (pmin, pmax) = Polygon.Project(polyVerts, axis);
            // Project the AABB's four corners onto this axis
            float c1 = aabb.Left  * axis.X + aabb.Top    * axis.Y;
            float c2 = aabb.Right * axis.X + aabb.Top    * axis.Y;
            float c3 = aabb.Right * axis.X + aabb.Bottom * axis.Y;
            float c4 = aabb.Left  * axis.X + aabb.Bottom * axis.Y;
            float amin = MathF.Min(MathF.Min(c1, c2), MathF.Min(c3, c4));
            float amax = MathF.Max(MathF.Max(c1, c2), MathF.Max(c3, c4));
            if (pmax < amin || amax < pmin) return false;
        }

        // World X axis: AABB span is [Left, Right]; polygon span = bounds of vertex Xs
        float pminX = polyVerts[0].X, pmaxX = polyVerts[0].X;
        float pminY = polyVerts[0].Y, pmaxY = polyVerts[0].Y;
        for (int i = 1; i < polyVerts.Length; i++)
        {
            var v = polyVerts[i];
            if (v.X < pminX) pminX = v.X; else if (v.X > pmaxX) pmaxX = v.X;
            if (v.Y < pminY) pminY = v.Y; else if (v.Y > pmaxY) pmaxY = v.Y;
        }
        if (pmaxX < aabb.Left || aabb.Right  < pminX) return false;
        if (pmaxY < aabb.Top  || aabb.Bottom < pminY) return false;

        return true;
    }
}
