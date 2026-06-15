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
// Instance-owned (not static): the (HitId → already-hit targets) dedupe table
// persists across frames, so it's simulation state that must be snapshot/restored
// for rollback. One CombatSystem per Simulation. The geometry helper below stays
// static — it's a pure function.
public sealed class CombatSystem
{
    private readonly Dictionary<int, HashSet<EntityId>> _hitDedupe = new();
    private readonly HashSet<int> _liveHitIds = new();
    private readonly List<int> _scratchPrune = new();
    // Per-frame recoil tally — accumulated impulse to apply to each hitbox's
    // attacker, keyed by HitId. Cleared at the start of every Apply (per-step
    // transient; not snapshotted). Attackers read via PeekRecoil in their
    // ApplyActionForces hook — that runs in frame N+1 before frame N+1's Apply
    // clears the dict, so the tally is effectively a 1-frame inbox from
    // CombatSystem to the action layer.
    private readonly Dictionary<int, Vector2> _recoilByHitId = new();
    // Per-frame hit-confirm tally — count of ENTITIES a hitbox connected with this
    // frame, keyed by HitId. Same 1-frame-inbox lifecycle as _recoilByHitId: cleared
    // at the start of every Apply, populated as entity hits land, read the next frame
    // by the attacker's action. Because entity hits are deduped per (HitId, Target),
    // a given target only counts on the frame it first connects — so an attack that
    // polls PeekHits each of its active/recovery frames sees the connection exactly
    // once and can latch a "did I hit?" flag (COMBAT_FEEL_PLAN Phase 3 hit-confirm).
    private readonly Dictionary<int, int> _entityHitsByHitId = new();

    public Vector2 PeekRecoil(int hitId)
        => _recoilByHitId.TryGetValue(hitId, out var v) ? v : Vector2.Zero;

    // Entities this HitId connected with on the LAST resolved frame. Read by an
    // attacker's action (one frame after the hit) to confirm a strike landed —
    // e.g. to gate a combo follow-up on a hit (whiff-punish), per Phase 3.
    public int PeekHits(int hitId)
        => _entityHitsByHitId.TryGetValue(hitId, out var n) ? n : 0;

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

        foreach (var hit in hitboxes.All)
        {
            _liveHitIds.Add(hit.HitId);

            // Narrow-phase polygon shape — compute world-space vertices + edge axes
            // once per hitbox, reused across all tile / entity overlap checks.
            Vector2[] hitVerts = null;
            Vector2[] hitAxes  = null;
            if (hit.Shape != null)
            {
                hitVerts = hit.Shape.GetVertices(hit.ShapePos, hit.ShapeRotation);
                hitAxes  = hit.Shape.GetAxes(hitVerts);
            }

            // --- Tile path (cumulative; same as the old DamageSystem) -----------
            if (hit.Targets != HitTargets.EntitiesOnly)
            {
                int gtx0 = (int)MathF.Floor(hit.Region.Left   / Chunk.TileSize);
                int gtx1 = (int)MathF.Floor(hit.Region.Right  / Chunk.TileSize);
                int gty0 = (int)MathF.Floor(hit.Region.Top    / Chunk.TileSize);
                int gty1 = (int)MathF.Floor(hit.Region.Bottom / Chunk.TileSize);
                for (int gtx = gtx0; gtx <= gtx1; gtx++)
                for (int gty = gty0; gty <= gty1; gty++)
                {
                    if (hit.Shape != null)
                    {
                        var tileAABB = new BoundingBox(
                            gtx * Chunk.TileSize,       gty * Chunk.TileSize,
                            (gtx + 1) * Chunk.TileSize, (gty + 1) * Chunk.TileSize);
                        if (!OverlapsPolyAABB(hitVerts, hitAxes, tileAABB)) continue;
                    }
                    // Snapshot the material BEFORE DamageCell, since a break clears
                    // the cell and we'd lose the type info for the hardness gate.
                    var typeBefore  = chunks.GetCellType(gtx, gty);
                    var stateBefore = chunks.GetCellState(gtx, gty);
                    // DamageCell returns true iff the tile broke this call.
                    bool broke = chunks.DamageCell(gtx, gty, hit.Damage);
                    if (hit.RecoilScale > 0f && stateBefore == TileState.Solid)
                    {
                        // Two gates, both must allow before crediting recoil:
                        //   BreakProtected ⇒ skip cells that this hit destroyed.
                        //   MinMaterialHP  ⇒ skip cells whose material is below the
                        //                    hardness floor (e.g. sand under a stab).
                        bool breakAllows = !hit.RecoilBreakProtected || !broke;
                        bool hardEnough  = TileDamage.MaxHPFor(typeBefore) > hit.RecoilMinMaterialHP;
                        if (breakAllows && hardEnough)
                            AccumulateRecoil(hit.HitId, -hit.KnockbackImpulse * hit.RecoilScale);
                    }
                }
            }

            // --- Entity path (deduped per (HitId, Target)) ----------------------
            if (hit.Targets != HitTargets.TilesOnly)
            {
                if (!_hitDedupe.TryGetValue(hit.HitId, out var alreadyHit))
                {
                    alreadyHit = new HashSet<EntityId>();
                    _hitDedupe[hit.HitId] = alreadyHit;
                }
                foreach (var hb in hurtboxes.All)
                {
                    if (hit.Owner == hb.Owner) continue;
                    if (alreadyHit.Contains(hb.Target)) continue;
                    if (!HitboxWorld.Overlaps(hit.Region, hb.Region)) continue;
                    if (hit.Shape != null && !OverlapsPolyAABB(hitVerts, hitAxes, hb.Region)) continue;
                    var target = resolve(hb.Target);
                    if (target == null) continue;   // owner despawned this frame — skip
                    target.OnHit(hit, hb);
                    alreadyHit.Add(hb.Target);
                    _entityHitsByHitId.TryGetValue(hit.HitId, out var prevHits);
                    _entityHitsByHitId[hit.HitId] = prevHits + 1;
                    if (hit.RecoilScale > 0f)
                        AccumulateRecoil(hit.HitId, -hit.KnockbackImpulse * hit.RecoilScale);
                }
            }
        }

        // Prune dedupe entries for HitIds that didn't broadcast this frame —
        // their attack is over, future hitboxes will use fresh HitIds anyway.
        _scratchPrune.Clear();
        foreach (var k in _hitDedupe.Keys)
            if (!_liveHitIds.Contains(k)) _scratchPrune.Add(k);
        foreach (var k in _scratchPrune) _hitDedupe.Remove(k);
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
    // _recoilByHitId is the same kind of inbox but is currently always empty
    // (RecoilScale = 0 everywhere); if that changes it needs the same treatment.
    public Dictionary<int, int> CaptureHitConfirm() => new(_entityHitsByHitId);

    public void RestoreHitConfirm(Dictionary<int, int> data)
    {
        _entityHitsByHitId.Clear();
        if (data == null) return;
        foreach (var (hitId, n) in data) _entityHitsByHitId[hitId] = n;
    }

    private void AccumulateRecoil(int hitId, Vector2 impulse)
    {
        _recoilByHitId.TryGetValue(hitId, out var cur);
        _recoilByHitId[hitId] = cur + impulse;
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
