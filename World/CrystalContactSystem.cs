using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// What a crystal wave does to bodies (todo.txt #7; dials in CrystalWave.cs). Runs once per
// Simulation.Step right after the spike contacts, so it sees where bodies came to rest.
//
//   FLASH (forming)  — a body overlapping the growing shard is shoved out along the
//                      shard's growth normal (its speed along it raised to PushSpeed).
//                      No damage: forming crystals are telegraph, not attack.
//   GROWN            — the only damaging phase. A body touching a grown shard takes
//                      CrystalDamage, capped at WaveDamageCap per victim per wave, and the
//                      crystal that landed BREAKS. The hit is terrain-occluded from the
//                      shard's base (TileReach), so a grown crystal can't bite through a
//                      wall it happens to poke into.
//
// Same delivery model as SpikeContactSystem (read its header): detection here, the landed
// hit delivered through IHittable.OnHit with a synthesized Hitbox so guard, invulnerability,
// hitstun and knockback all apply as for any attack. The launcher's side is immune to
// both channels (SpikeContactSystem.Opposes), consistent with spikes.
//
// Stateless across frames: the per-wave tally lives in CrystalWaveField (terrain
// snapshot). Victims are walked in canonical hurtbox order and cells in grid order, so
// the result is independent of the crystal table's dictionary order.
public sealed class CrystalContactSystem
{
    private readonly HashSet<EntityId> _seen = new();

    // Returns the number of crystal hits landed this frame (tests/diagnostics).
    public int Apply(ChunkMap chunks, HurtboxWorld hurtboxes,
                     Func<EntityId, IHittable> resolve, Func<EntityId, PhysicsBody> bodyOf,
                     HitIdAllocator hitIds, bool coop)
    {
        var field = chunks.Crystals;
        if (field.Count == 0) return 0;
        int now = chunks.TerrainFrame;
        int landed = 0;
        _seen.Clear();
        foreach (var hb in hurtboxes.All)
        {
            if (!_seen.Add(hb.Target)) continue;
            var target = resolve(hb.Target);
            var body   = bodyOf(hb.Target);
            if (target == null || body == null || body.IgnoreTiles) continue;
            int team = target switch
            {
                PlayerCharacter p => p.Team,
                Entity e          => e.Team,
                _                 => -1,
            };

            // A grounded player's feet reach below the torso AABB (SpikeContactSystem).
            var r = hb.Region;
            if (target is PlayerCharacter pc && pc.IsGrounded)
                r = new BoundingBox(r.Left, r.Top, r.Right, r.Bottom + SpikeContactSystem.FootReach);

            TryPush(field, chunks, r, body, hb.Owner, team, coop, now);

            if (!FindGrown(field, chunks, r, hb.Owner, team, coop, hb.Target, now,
                           out int gx, out int gy, out var crystal, out float damage)) continue;

            var n = CrystalWave.Normal(crystal.Base);
            var hit = new Hitbox(hb.Region, hitIds.Next(), damage: 0f,
                                 knockbackImpulse: n * CrystalWave.KnockbackImpulse,
                                 owner: crystal.Side.Faction, source: EntityId.None,
                                 debugColor: Color.LightCyan, targets: HitTargets.EntitiesOnly,
                                 bodyDamage: damage, hitstunSecondsOverride: CrystalWave.HitstunSeconds,
                                 strikeDir: n);
            target.OnHit(in hit, in hb);
            field.AddTally(crystal.Wave, hb.Target, damage, now);
            field.Break(gx, gy, now);
            landed++;
        }
        return landed;
    }

    // Cells whose shard could reach `r`: the shard stands on a face of its own cell and
    // extends up to GrownLength, so widen the scan by that much.
    private static void ScanRange(BoundingBox r, out int gx0, out int gx1, out int gy0, out int gy1)
    {
        const float T = Chunk.TileSize;
        float pad = CrystalWave.GrownLength + CrystalWave.ContactSlop;
        gx0 = (int)MathF.Floor((r.Left   - pad) / T);
        gx1 = (int)MathF.Floor((r.Right  + pad) / T);
        gy0 = (int)MathF.Floor((r.Top    - pad) / T);
        gy1 = (int)MathF.Floor((r.Bottom + pad) / T);
    }

    private static bool Overlaps(BoundingBox a, BoundingBox b, float slop)
        => a.Left - slop < b.Right && b.Left < a.Right + slop
        && a.Top  - slop < b.Bottom && b.Top < a.Bottom + slop;

    // Forming crystals shove an opposing body out of their way. One shove per body per
    // frame (the first forming shard in grid order). A grown neighbour can still bite
    // the same frame — the push is the forming shard's only effect, not an immunity.
    private static bool TryPush(CrystalWaveField field, ChunkMap chunks, BoundingBox r, PhysicsBody body,
                                Faction faction, int team, bool coop, int now)
    {
        ScanRange(r, out int gx0, out int gx1, out int gy0, out int gy1);
        for (int gy = gy0; gy <= gy1; gy++)
        for (int gx = gx0; gx <= gx1; gx++)
        {
            if (!field.TryGet(gx, gy, out var c) || c.Pending || c.Broken) continue;
            int elapsed = now - c.SeededFrame;
            var life = c.Lifecycle;
            if (life.PhaseAt(elapsed) != CrystalPhase.Flash) continue;
            if (!SpikeContactSystem.Opposes(in c.Side, faction, team, coop)) continue;
            var box = CrystalWave.ShardBox(gx, gy, c.Base, CrystalWave.LengthAt(in life, elapsed));
            if (!Overlaps(box, r, 0f)) continue;
            var n = CrystalWave.Normal(c.Base);
            float along = Vector2.Dot(body.Velocity, n);
            if (along < CrystalWave.PushSpeed) body.Velocity += (CrystalWave.PushSpeed - along) * n;
            return true;
        }
        return false;
    }

    // The first grown, opposing, reachable crystal touching `r` in grid order, provided
    // the victim still has cap room under that crystal's wave and isn't inside the
    // wave's re-hit spacing. `damage` is what it may take.
    private static bool FindGrown(CrystalWaveField field, ChunkMap chunks, BoundingBox r,
                                  Faction faction, int team, bool coop, EntityId victim, int now,
                                  out int hx, out int hy, out CrystalWaveField.Crystal hit, out float damage)
    {
        hx = hy = 0; hit = default; damage = 0f;
        ScanRange(r, out int gx0, out int gx1, out int gy0, out int gy1);
        for (int gy = gy0; gy <= gy1; gy++)
        for (int gx = gx0; gx <= gx1; gx++)
        {
            if (!field.TryGet(gx, gy, out var c) || c.Pending || c.Broken) continue;
            if (c.PhaseAt(now) != CrystalPhase.Grown) continue;
            if (!SpikeContactSystem.Opposes(in c.Side, faction, team, coop)) continue;
            var box = CrystalWave.ShardBox(gx, gy, c.Base, CrystalWave.GrownLength);
            if (!Overlaps(box, r, CrystalWave.ContactSlop)) continue;
            var tally = field.TallyFor(c.Wave, victim);
            float room = CrystalWave.WaveDamageCap - tally.Dealt;
            if (room <= 1e-4f) continue;                                   // capped: harmless
            if (now - tally.LastHitFrame < CrystalWave.ReHitFrames) continue;
            if (!TileReach.IsRegionReachable(chunks, CrystalWave.OcclusionOrigin(gx, gy, c.Base), r)) continue;
            hx = gx; hy = gy; hit = c;
            damage = MathF.Min(CrystalWave.CrystalDamage, room);
            return true;
        }
        return false;
    }
}
