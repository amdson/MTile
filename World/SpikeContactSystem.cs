using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Contact damage from spiked cells (Plans/BLOCK_OWNERSHIP_SPIKES.md §2). Runs once per
// Simulation.Step, right after the physics sweep, so it sees where bodies actually came
// to rest this frame — and, via CapturePreStep, how fast they were going on the way in.
//
// Why a dedicated system rather than publishing Hitboxes into CombatSystem: the hitbox
// path dedupes per (HitId, Target) for as long as a HitId keeps broadcasting, and a
// hitbox carries ONE damage number for everyone it overlaps. Spikes want the opposite
// on both counts — a per-victim re-arming cooldown, and damage that depends on each
// victim's own impact speed. So detection, side filtering and the cooldown live here,
// and the landed tick is delivered through the ordinary IHittable.OnHit with a
// synthesized Hitbox. That keeps every downstream rule of a real hit — invulnerability,
// guard, superarmor, hitstun, knockback through HitResolver, the regen-delay stamp —
// exactly as for any attack; and because only published hurtboxes are candidates, an
// invulnerable (hurtbox-suppressed) player is skipped for free.
//
// State: the per-victim last-hit frame (snapshotted, checksummed). The pre-step
// velocity map is per-Step scratch, filled and consumed inside the same Step, so it is
// always stale-but-unused at a frame boundary and needs no snapshot.
public sealed class SpikeContactSystem
{
    // ── Tuning (spec starting numbers) ──────────────────────────────────────────
    // HP taken by a gentle contact: standing on spikes, walking into them.
    public const float ContactDamage    = 0.25f;
    // HP taken at (and above) FullImpactSpeed of normal approach — being knocked into
    // spikes. Linear between the two.
    public const float MaxImpactDamage  = 1.5f;
    // Normal approach speed (px/s) at or below which a contact counts as "walking"
    // and deals ContactDamage. At or above the movement config's MaxWalkSpeed (80), so
    // strolling into a spiked wall reads the same as standing on a spiked floor.
    public const float RampStartSpeed   = 100f;
    public const float FullImpactSpeed  = 400f;
    // Per-victim re-arm time: contact ticks, it doesn't shred.
    public const float CooldownSeconds  = 0.5f;
    // Knockback impulse away from the spiked face (Impulse mode; the victim's mass
    // divides it, so ~100 px/s on a 2.5-mass player). Kept under
    // CombatState.BigHitStrength (280) so a tick never stuns or hitstops.
    public const float KnockbackImpulse = 250f;
    // Short fixed hitstun per tick — long enough to register as a hit, short enough
    // (well under CooldownSeconds) that a player standing on spikes can still jump off.
    public const float HitstunSeconds   = 0.12f;
    // How far (px) outside a body's AABB a spiked face still counts as touching. Covers
    // the standing spring's hover over the floor and the solver's resting skin.
    public const float ContactSlop      = 3f;
    // How far below a GROUNDED player's torso AABB its feet reach: the stand fold's
    // hover (MovementConfig.FoldHoverOffset, ~15 px) plus one slop. Read live so a
    // retuned hover keeps spikes underfoot in reach.
    public static float FootReach => MovementConfig.Current.FoldHoverOffset + ContactSlop;

    private readonly Dictionary<EntityId, int> _lastHitFrame = new();
    private readonly Dictionary<PhysicsBody, Vector2> _preStepVelocity =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<EntityId> _seenThisFrame = new();

    // Fraction of the way from walking contact to a full-speed impact.
    public static float DamageForSpeed(float normalSpeed)
    {
        float t = (normalSpeed - RampStartSpeed) / (FullImpactSpeed - RampStartSpeed);
        t = Math.Clamp(t, 0f, 1f);
        return ContactDamage + (MaxImpactDamage - ContactDamage) * t;
    }

    // Does a spike owned by `side` hurt a body of (faction, team)? Placer's side never;
    // co-op players never hurt each other; two Enemy-faction parties fall back to team.
    public static bool Opposes(in TileSpikes.Side side, Faction faction, int team, bool coop)
    {
        if (coop && Factions.IsPlayer(side.Faction) && Factions.IsPlayer(faction)) return false;
        if (side.Faction != faction) return true;
        return side.Faction == Faction.Enemy && side.Team != team;
    }

    // Record every body's velocity before the physics sweep zeroes the normal component
    // of whatever it hits. Called by Simulation immediately before StepSwept.
    public void CapturePreStep(IReadOnlyList<PhysicsBody> bodies)
    {
        _preStepVelocity.Clear();
        for (int i = 0; i < bodies.Count; i++) _preStepVelocity[bodies[i]] = bodies[i].Velocity;
    }

    // Resolve spike contacts for this frame. Hurtboxes are walked in their canonical
    // (published) order, so the hit-id draw order — and everything downstream — is
    // deterministic. Returns the number of ticks landed (tests/diagnostics).
    public int Apply(ChunkMap chunks, HurtboxWorld hurtboxes,
                     Func<EntityId, IHittable> resolve, Func<EntityId, PhysicsBody> bodyOf,
                     HitIdAllocator hitIds, int frame, bool coop)
    {
        if (chunks.Spikes.Count == 0) { PruneCooldowns(frame); return 0; }

        int cooldownFrames = SimFrames.FromSeconds(CooldownSeconds, Simulation.FixedDt);
        int landed = 0;
        _seenThisFrame.Clear();
        foreach (var hb in hurtboxes.All)
        {
            // One evaluation per victim, however many hurtboxes it published.
            if (!_seenThisFrame.Add(hb.Target)) continue;
            if (_lastHitFrame.TryGetValue(hb.Target, out int last) && frame - last < cooldownFrames)
                continue;

            var target = resolve(hb.Target);
            var body   = bodyOf(hb.Target);
            if (target == null || body == null || body.IgnoreTiles) continue;
            int team = target switch
            {
                PlayerCharacter p => p.Team,
                Entity e          => e.Team,
                _                 => -1,
            };

            // A grounded player's torso hovers over the floor it stands on (the stand fold
            // never physically touches it) — its feet reach FootReach below the AABB.
            float footReach = target is PlayerCharacter pc && pc.IsGrounded ? FootReach : 0f;
            if (!FindContact(chunks, body, footReach, hb.Owner, team, coop, out var normal, out var side)) continue;

            _preStepVelocity.TryGetValue(body, out var vin);
            float speed  = MathF.Max(0f, -Vector2.Dot(vin, normal));
            float damage = DamageForSpeed(speed);

            var hit = new Hitbox(hb.Region, hitIds.Next(), damage: 0f,
                                 knockbackImpulse: normal * KnockbackImpulse,
                                 owner: side.Faction, source: EntityId.None,
                                 debugColor: Color.OrangeRed, targets: HitTargets.EntitiesOnly,
                                 bodyDamage: damage, hitstunSecondsOverride: HitstunSeconds,
                                 strikeDir: normal);
            target.OnHit(in hit, in hb);
            _lastHitFrame[hb.Target] = frame;
            landed++;
        }
        PruneCooldowns(frame);
        return landed;
    }

    // The worst (most-opposing-approach) spiked face this body is touching. The face's
    // outward normal points from the cell toward the body.
    private bool FindContact(ChunkMap chunks, PhysicsBody body, float footReach, Faction faction, int team,
                             bool coop, out Vector2 normal, out TileSpikes.Side side)
    {
        normal = default; side = default;
        var b = ContactBounds(body, footReach);
        const float T = Chunk.TileSize;
        int gx0 = (int)MathF.Floor((b.Left   - ContactSlop) / T);
        int gx1 = (int)MathF.Floor((b.Right  + ContactSlop) / T);
        int gy0 = (int)MathF.Floor((b.Top    - ContactSlop) / T);
        int gy1 = (int)MathF.Floor((b.Bottom + ContactSlop) / T);
        _preStepVelocity.TryGetValue(body, out var vin);

        bool found = false;
        float bestSpeed = -1f;
        for (int gy = gy0; gy <= gy1; gy++)
        for (int gx = gx0; gx <= gx1; gx++)
        {
            if (!chunks.Spikes.TryGet(gx, gy, out var s)) continue;
            if (chunks.GetCellState(gx, gy) != TileState.Solid) continue;
            if (!Opposes(in s, faction, team, coop)) continue;

            float cl = gx * T, cr = cl + T, ct = gy * T, cb = ct + T;
            // Signed separations: positive = gap, negative = overlap, on each axis.
            float gapL = cl - b.Right;   // body left of the cell
            float gapR = b.Left - cr;    // body right of the cell
            float gapT = ct - b.Bottom;  // body above the cell
            float gapB = b.Top - cb;     // body below the cell
            float sx = MathF.Max(gapL, gapR);
            float sy = MathF.Max(gapT, gapB);
            if (sx > ContactSlop || sy > ContactSlop) continue;

            // Face contact only: the body must overlap the face's span on the tangent
            // axis (a pure corner brush doesn't count), and the face must be exposed —
            // a spiked cell's face buried against another solid cell can't be touched.
            Vector2 n; int nx, ny;
            if (sx > sy) { if (sy >= 0f) continue; nx = gapL > gapR ? -1 : 1; ny = 0; }
            else         { if (sx >= 0f) continue; nx = 0; ny = gapT > gapB ? -1 : 1; }
            if (chunks.GetCellState(gx + nx, gy + ny) == TileState.Solid) continue;
            n = new Vector2(nx, ny);

            float speed = MathF.Max(0f, -Vector2.Dot(vin, n));
            if (speed > bestSpeed) { bestSpeed = speed; normal = n; side = s; found = true; }
        }
        return found;
    }

    // The body's AABB, stretched out to the surfaces it is riding rather than touching.
    // A player's collision polygon is the torso, and it never physically rests on what
    // supports it: the stand fold holds it ~FoldHoverOffset above the floor (footReach,
    // passed for a grounded player), and the older spring states hold it off a surface
    // with a FloatingSurfaceDistance (extended to that contact's surface point). Without
    // this, the bare AABB never "touches" the floor a player is standing on. Bodies with
    // neither (entities) use their plain AABB.
    private static BoundingBox ContactBounds(PhysicsBody body, float footReach)
    {
        var b = body.Bounds;
        float l = b.Left, t = b.Top, r = b.Right, btm = b.Bottom + footReach;
        foreach (var c in body.Constraints)
        {
            if (c is not FloatingSurfaceDistance f) continue;
            var n = f.Normal;
            if (MathF.Abs(n.Y) >= MathF.Abs(n.X))
            {
                if (n.Y < 0f) btm = MathF.Max(btm, f.Position.Y);   // floor below
                else          t   = MathF.Min(t,   f.Position.Y);   // ceiling above
            }
            else
            {
                if (n.X < 0f) r = MathF.Max(r, f.Position.X);       // wall to the right
                else          l = MathF.Min(l, f.Position.X);       // wall to the left
            }
        }
        return new BoundingBox(l, t, r, btm);
    }

    private readonly List<EntityId> _pruneScratch = new();
    private void PruneCooldowns(int frame)
    {
        if (_lastHitFrame.Count == 0) return;
        int cooldownFrames = SimFrames.FromSeconds(CooldownSeconds, Simulation.FixedDt);
        _pruneScratch.Clear();
        foreach (var (id, last) in _lastHitFrame)
            if (frame - last >= cooldownFrames) _pruneScratch.Add(id);
        foreach (var id in _pruneScratch) _lastHitFrame.Remove(id);
    }

    // ── Snapshot / restore / checksum ───────────────────────────────────────────
    public Dictionary<EntityId, int> CaptureCooldowns() => new(_lastHitFrame);

    public void RestoreCooldowns(Dictionary<EntityId, int> src)
    {
        _lastHitFrame.Clear();
        if (src == null) return;
        foreach (var kv in src) _lastHitFrame[kv.Key] = kv.Value;
    }

    public IEnumerable<KeyValuePair<EntityId, int>> Cooldowns => _lastHitFrame;
}
